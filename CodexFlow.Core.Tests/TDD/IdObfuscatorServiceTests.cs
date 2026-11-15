// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Xunit;
using FluentAssertions;
using System;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Services;

namespace CodexFlow.Core.Tests.TDD;

public class IdObfuscatorServiceTests
{
    private readonly IIdObfuscatorService _service;

    public IdObfuscatorServiceTests()
    {
        _service = new IdObfuscatorService();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999999)]
    public void Encode_ShouldReturnNonEmptyString_WhenIdIsValid(long id)
    {
        // Act
        var result = _service.Encode(id);

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Length.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Encode_ShouldThrowArgumentException_WhenIdIsZeroOrNegative()
    {
        // Act
        Action act = () => _service.Encode(0);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("*must be positive*");
    }

    [Fact]
    public void RoundTrip_ShouldReturnOriginalId()
    {
        // Arrange
        long originalId = 123456789;

        // Act
        var encoded = _service.Encode(originalId);
        var decoded = _service.Decode(encoded);

        // Assert
        decoded.Should().Be(originalId);
    }
}
