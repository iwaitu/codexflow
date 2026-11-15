// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// EditApplier 单元测试。
/// </summary>
public class EditApplierTests
{
    private readonly EditApplier _applier = new();

    private static FileSnapshot CreateSnapshot(string content)
    {
        // 检测是否有尾部换行
        var hasTrailingNewline = content.Length > 0 && content.EndsWith("\n", StringComparison.Ordinal);

        var lines = content.Split('\n').Select((line, index) => new HashlineRecord
        {
            LineNumber = index + 1,
            AnchorId = $"ANCHOR{index + 1}",
            RawText = line,
            NormalizedText = line
        }).ToList();

        return new FileSnapshot
        {
            SnapshotId = "test-snapshot",
            FilePath = "/test/file.txt",
            FileFingerprint = "test-fingerprint",
            EncodingName = "utf-8",
            HasBom = false,
            NewLineStyle = "\n",
            HasTrailingNewline = hasTrailingNewline,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Lines = lines,
            RenderedText = content
        };
    }

    [Fact]
    public void Apply_NoOperations_ReturnsOriginalContent()
    {
        var snapshot = CreateSnapshot("Line1\nLine2\nLine3");
        var operations = new List<EditOperation>();

        var result = _applier.Apply(snapshot, operations);

        Assert.Equal(snapshot.RenderedText, result.OldContent);
        Assert.Equal(snapshot.RenderedText, result.NewContent);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void Apply_InsertAfter_AddsLineAfterTarget()
    {
        var snapshot = CreateSnapshot("Line1\nLine2\nLine3");
        var operation = new InsertAfterOperation
        {
            TargetLine = 1,
            TargetAnchorId = "ANCHOR1",
            NewLines = new List<string> { "NewLine" }
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("NewLine", result.NewContent);
    }

    [Fact]
    public void Apply_InsertBefore_AddsLineBeforeTarget()
    {
        var snapshot = CreateSnapshot("Line1\nLine2\nLine3");
        var operation = new InsertBeforeOperation
        {
            TargetLine = 2,
            TargetAnchorId = "ANCHOR2",
            NewLines = new List<string> { "NewLine" }
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("NewLine", result.NewContent);
    }

    [Fact]
    public void Apply_ReplaceRange_ReplacesLines()
    {
        var snapshot = CreateSnapshot("Line1\nLine2\nLine3\nLine4");
        var operation = new ReplaceRangeOperation
        {
            StartLine = 2,
            StartAnchorId = "ANCHOR2",
            EndLine = 3,
            EndAnchorId = "ANCHOR3",
            NewLines = new List<string> { "ReplacedLine1", "ReplacedLine2" }
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("ReplacedLine1", result.NewContent);
        Assert.Contains("ReplacedLine2", result.NewContent);
    }

    [Fact]
    public void Apply_DeleteRange_RemovesLines()
    {
        var snapshot = CreateSnapshot("Line1\nLine2\nLine3\nLine4");
        var operation = new DeleteRangeOperation
        {
            StartLine = 2,
            StartAnchorId = "ANCHOR2",
            EndLine = 3,
            EndAnchorId = "ANCHOR3"
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("Line1", result.NewContent);
        Assert.Contains("Line4", result.NewContent);
        Assert.DoesNotContain("Line2", result.NewContent);
        Assert.DoesNotContain("Line3", result.NewContent);
    }

    [Fact]
    public void Apply_MultipleOperations_AppliesInCorrectOrder()
    {
        var snapshot = CreateSnapshot("A\nB\nC\nD\nE");

        // 先删除，再插入 - 注意倒序应用
        var operations = new List<EditOperation>
        {
            new DeleteRangeOperation
            {
                StartLine = 3,
                StartAnchorId = "ANCHOR3",
                EndLine = 3,
                EndAnchorId = "ANCHOR3"
            },
            new InsertAfterOperation
            {
                TargetLine = 1,
                TargetAnchorId = "ANCHOR1",
                NewLines = new List<string> { "X" }
            }
        };

        var result = _applier.Apply(snapshot, operations);

        // 应该包含插入的 X，且删除了 C
        Assert.Contains("X", result.NewContent);
        Assert.DoesNotContain("C", result.NewContent);
    }

    [Fact]
    public void Apply_RewriteWholeFile_ReplacesAllContent()
    {
        var snapshot = CreateSnapshot("OldContent1\nOldContent2");
        var operation = new RewriteWholeFileOperation
        {
            NewContent = "NewContent1\nNewContent2\nNewContent3"
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("NewContent1", result.NewContent);
        Assert.Contains("NewContent2", result.NewContent);
        Assert.Contains("NewContent3", result.NewContent);
        Assert.DoesNotContain("OldContent", result.NewContent);
    }

    [Fact]
    public void Apply_InsertMultipleLines_AddsAllLines()
    {
        var snapshot = CreateSnapshot("Line1");
        var operation = new InsertAfterOperation
        {
            TargetLine = 1,
            TargetAnchorId = "ANCHOR1",
            NewLines = new List<string> { "NewLine1", "NewLine2", "NewLine3" }
        };

        var result = _applier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("NewLine1", result.NewContent);
        Assert.Contains("NewLine2", result.NewContent);
        Assert.Contains("NewLine3", result.NewContent);
    }

    [Fact]
    public void Apply_ThrowsOnNullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() => _applier.Apply(null!, new List<EditOperation>()));
    }
}