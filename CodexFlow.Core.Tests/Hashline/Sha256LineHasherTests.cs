// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Hashline.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// Sha256LineHasher 单元测试。
/// </summary>
public class Sha256LineHasherTests
{
    private readonly Sha256LineHasher _hasher = new();

    [Fact]
    public void ComputeAnchorId_SameInput_ReturnsSameHash()
    {
        var hash1 = _hasher.ComputeAnchorId("Hello World");
        var hash2 = _hasher.ComputeAnchorId("Hello World");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeAnchorId_DifferentInput_ReturnsDifferentHash()
    {
        var hash1 = _hasher.ComputeAnchorId("Hello World");
        var hash2 = _hasher.ComputeAnchorId("Hello World!");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeAnchorId_Returns8CharacterUppercaseHex()
    {
        var hash = _hasher.ComputeAnchorId("Test Line");

        Assert.Equal(8, hash.Length);
        Assert.True(hash.All(c => char.IsAsciiHexDigitUpper(c)));
    }

    [Fact]
    public void ComputeAnchorId_EmptyString_ReturnsValidHash()
    {
        var hash = _hasher.ComputeAnchorId(string.Empty);

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }

    [Fact]
    public void ComputeAnchorId_NullInput_ReturnsValidHash()
    {
        var hash = _hasher.ComputeAnchorId(null!);

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }

    [Fact]
    public void ComputeAnchorId_WhitespaceOnly_ReturnsValidHash()
    {
        var hash = _hasher.ComputeAnchorId("   ");

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }

    [Fact]
    public void ComputeAnchorId_ConsistentHashForSameContent()
    {
        // 测试一致性：多次计算同一内容应该返回相同的哈希
        var content = "using System;";
        var hashes = Enumerable.Range(0, 10)
            .Select(_ => _hasher.ComputeAnchorId(content))
            .ToHashSet();

        Assert.Single(hashes);
    }

    [Fact]
    public void ComputeAnchorId_SpecialCharactersHandled()
    {
        var hash = _hasher.ComputeAnchorId("Line with \"quotes\" and 'apostrophes'");

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }

    [Fact]
    public void ComputeAnchorId_UnicodeCharactersHandled()
    {
        var hash = _hasher.ComputeAnchorId("Unicode: 你好世界 🌍");

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }

    [Fact]
    public void ComputeAnchorId_VeryLongLine_ReturnsValidHash()
    {
        var longLine = new string('a', 10000);
        var hash = _hasher.ComputeAnchorId(longLine);

        Assert.Equal(8, hash.Length);
        Assert.NotNull(hash);
    }
}