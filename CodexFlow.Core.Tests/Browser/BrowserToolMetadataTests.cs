// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Agents.Tools;
using Newtonsoft.Json;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace CodexFlow.Core.Tests.Browser;

public sealed class BrowserToolMetadataTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedMetadataPreservesLegacyJson(bool populated)
    {
        string? url = populated ? "https://example.invalid/page" : null;
        string? title = populated ? "测试页" : null;
        long? screenshotSeq = populated ? 123 : null;
        string? errorCode = populated ? "blocked" : null;

        AssertSameJson(
            new { Url = url, Title = title },
            new BrowserPageToolMetadata(url, title));
        AssertSameJson(
            new { Url = url, Title = title, screenshotSeq },
            new BrowserScreenshotToolMetadata(url, title, screenshotSeq));
        AssertSameJson(
            new { ErrorCode = errorCode },
            new BrowserFailureToolMetadata(errorCode));
    }

    private static void AssertSameJson(object legacy, object current)
    {
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(current));
        Assert.Equal(JsonSerializer.Serialize(legacy, WebJson), JsonSerializer.Serialize(current, WebJson));
        Assert.Equal(JsonConvert.SerializeObject(legacy), JsonConvert.SerializeObject(current));
    }
}
