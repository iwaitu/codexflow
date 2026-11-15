// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Hashline.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// TextNormalizer 单元测试。
/// </summary>
public class TextNormalizerTests
{
    private readonly TextNormalizer _normalizer = new();

    [Fact]
    public void Normalize_EmptyString_ReturnsEmptyResult()
    {
        var result = _normalizer.Normalize(string.Empty);

        Assert.Equal(string.Empty, result.NormalizedText);
        Assert.Equal("\n", result.DetectedNewLineStyle);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Normalize_NullString_ReturnsEmptyResult()
    {
        var result = _normalizer.Normalize(null!);

        Assert.Equal(string.Empty, result.NormalizedText);
        Assert.Equal("\n", result.DetectedNewLineStyle);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Normalize_SingleLine_ReturnsCorrectResult()
    {
        var result = _normalizer.Normalize("Hello World");

        Assert.Equal("Hello World", result.NormalizedText);
        Assert.Equal("\n", result.DetectedNewLineStyle);
        Assert.Single(result.Lines);
        Assert.Equal("Hello World", result.Lines[0]);
    }

    [Fact]
    public void Normalize_MultipleLines_ReturnsCorrectResult()
    {
        var result = _normalizer.Normalize("Line1\nLine2\nLine3");

        Assert.Equal("Line1\nLine2\nLine3", result.NormalizedText);
        Assert.Equal("\n", result.DetectedNewLineStyle);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("Line1", result.Lines[0]);
        Assert.Equal("Line2", result.Lines[1]);
        Assert.Equal("Line3", result.Lines[2]);
    }

    [Fact]
    public void Normalize_CRLF_DetectsCorrectNewLineStyle()
    {
        var result = _normalizer.Normalize("Line1\r\nLine2\r\nLine3");

        Assert.Equal("\r\n", result.DetectedNewLineStyle);
        Assert.Equal(3, result.Lines.Count);
    }

    [Fact]
    public void Normalize_MixedLineEndings_NormalizesToLF()
    {
        var result = _normalizer.Normalize("Line1\r\nLine2\nLine3");

        Assert.DoesNotContain("\r\n", result.NormalizedText);
        Assert.Equal(3, result.Lines.Count);
    }

    [Fact]
    public void Normalize_TrailingCarriageReturn_RemovesCarriageReturn()
    {
        var result = _normalizer.Normalize("Line\r");

        Assert.Equal("Line", result.NormalizedText);
        Assert.Single(result.Lines);
        Assert.Equal("Line", result.Lines[0]);
    }

    [Fact]
    public void NormalizeLine_WithTrailingCarriageReturn_RemovesIt()
    {
        var result = _normalizer.NormalizeLine("Hello\r");

        Assert.Equal("Hello", result);
    }

    [Fact]
    public void NormalizeLine_EmptyString_ReturnsEmptyString()
    {
        var result = _normalizer.NormalizeLine(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SplitLines_SingleLine_ReturnsSingleElement()
    {
        var result = _normalizer.SplitLines("SingleLine");

        Assert.Single(result);
        Assert.Equal("SingleLine", result[0]);
    }

    [Fact]
    public void SplitLines_EmptyString_ReturnsEmpty()
    {
        var result = _normalizer.SplitLines(string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void SplitLines_LastLineWithoutNewline_Included()
    {
        var result = _normalizer.SplitLines("Line1\nLine2");

        Assert.Equal(2, result.Count);
        Assert.Equal("Line1", result[0]);
        Assert.Equal("Line2", result[1]);
    }

    [Fact]
    public void Normalize_PreservesLeadingWhitespace()
    {
        var result = _normalizer.Normalize("  IndentedLine");

        Assert.Equal("  IndentedLine", result.NormalizedText);
        Assert.Equal("  IndentedLine", result.Lines[0]);
    }

    [Fact]
    public void Normalize_PreservesTrailingWhitespace()
    {
        var result = _normalizer.Normalize("Trailing  ");

        Assert.Equal("Trailing  ", result.NormalizedText);
        Assert.Equal("Trailing  ", result.Lines[0]);
    }

    [Fact]
    public void Normalize_PreservesTabs()
    {
        var result = _normalizer.Normalize("\tTabbedLine");

        Assert.Equal("\tTabbedLine", result.NormalizedText);
        Assert.Equal("\tTabbedLine", result.Lines[0]);
    }

    [Fact]
    public void Normalize_EmptyLinesPreserved()
    {
        var result = _normalizer.Normalize("Line1\n\nLine3");

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("Line1", result.Lines[0]);
        Assert.Equal(string.Empty, result.Lines[1]);
        Assert.Equal("Line3", result.Lines[2]);
    }
}