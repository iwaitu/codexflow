// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Gateway;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class ToolEventSanitizerTests
{
    [Fact]
    public void SanitizeArguments_PreservesPrimitiveTypesAndRedactsNestedValues()
    {
        object?[] values =
        [
            null, true, (byte)2, (sbyte)-3, (short)-4, (ushort)5, -6, 7U,
            long.MinValue, ulong.MaxValue, 1.25F, 3.5D, 0.1234567890123456789012345678M
        ];
        using var json = JsonDocument.Parse("""[{"API_KEY":"synthetic-value","line":"Bearer example-token01"}]""");
        var (sanitized, truncated) = ToolEventSanitizer.SanitizeArguments(new Dictionary<string, object?>
        {
            ["values"] = values,
            ["details"] = new Dictionary<string, object?>
            {
                ["password"] = "synthetic-value",
                ["PasswordStatus"] = "synthetic-status"
            },
            ["json"] = json.RootElement
        });

        Assert.False(truncated);
        var normalized = Assert.IsType<Dictionary<string, object?>>(sanitized);
        var numbers = Assert.IsType<List<object?>>(normalized["values"]);
        Assert.Equal(values, numbers);
        Assert.Equal(values.Select(value => value?.GetType()), numbers.Select(value => value?.GetType()));
        var details = Assert.IsType<Dictionary<string, object?>>(normalized["details"]);
        Assert.Equal(new[] { "password", "PasswordStatus" }, details.Keys);
        Assert.All(details.Values, item => Assert.Equal(ToolEventSanitizer.RedactedValue, item));
        var jsonItem = Assert.Single(Assert.IsType<List<object?>>(normalized["json"]));
        var jsonObject = Assert.IsType<Dictionary<string, object?>>(jsonItem);
        Assert.Equal(ToolEventSanitizer.RedactedValue, jsonObject["API_KEY"]);
        Assert.Equal("Bearer [REDACTED]", jsonObject["line"]);
    }

    [Fact]
    public void SanitizeArguments_CompactionCountsEscapedTextAndHandlesNull()
    {
        var first = new string('中', 38) + "\n\"";
        var (sanitized, truncated) = ToolEventSanitizer.SanitizeArguments(new Dictionary<string, object?>
        {
            ["央"] = first,
            ["empty"] = null,
            ["tail"] = new string('中', 42),
            ["afterTail"] = "omitted"
        }, maxTotalJsonChars: 400);

        Assert.True(truncated);
        var compact = Assert.IsType<Dictionary<string, object?>>(sanitized);
        Assert.Equal(new[] { "央", "empty", "tail" }, compact.Keys);
        Assert.Equal(first, compact["央"]);
        Assert.Null(compact["empty"]);
        Assert.Equal("[TRUNCATED]", compact["tail"]);
    }

    [Fact]
    public void SanitizeArguments_PreservesNonFiniteNumberRejection()
    {
        Assert.Throws<ArgumentException>(() =>
            ToolEventSanitizer.SanitizeArguments(new Dictionary<string, object?> { ["value"] = float.NaN }));
        Assert.Throws<ArgumentException>(() =>
            ToolEventSanitizer.SanitizeArguments(new Dictionary<string, object?> { ["value"] = double.PositiveInfinity }));
    }
}
